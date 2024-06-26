﻿#if INTERACTIVE
#r "nuget: torchsharp-cuda-windows" //, 0.98.1" //for gpu
#r "nuget: TorchSharp" //, 0.98.1"
#r "nuget: TfCheckpoint"   
#r "nuget: FsBERTTokenizer"
#r "nuget: FSharp.Data, 5.0.2"
#r "nuget: FAkka.FsPickler"
#endif

open System.Collections.Generic
open System.IO
open TfCheckpoint
open TorchSharp
open System.Collections.Concurrent
open MBrace.FsPickler

let binarySerializer = FsPickler.CreateBinarySerializer()


let device = if torch.cuda_is_available() then torch.CUDA else torch.CPU
printfn $"torch devices is %A{device}"

//let bertCheckpointFolder = @"C:\temp\bert\variables"
//let tensors = CheckpointReader.readCheckpoint bertCheckpointFolder |> Seq.toArray
//show first tensor
//printfn "%A" tensors.[0]


//tensors (* |> Array.skip 20 *) |> Array.map (fun (n,st) -> {|Dims=st.Shape; Name=n|})

let weightDataDir = @"C:\anibal\ttc\MyTrainedModel\yelp_review_all_csv"

//tensor dims - these values should match the relevant dimensions of the corresponding tensors in the checkpoint
let HIDDEN      = 128L
let VOCAB_SIZE  = 30522L    // see vocab.txt file included in the BERT download
let TYPE_SIZE   = 2L         // bert needs 'type' of token
let MAX_POS_EMB = 512L

//other parameters
let EPS_LAYER_NORM      = 1e-12
let HIDDEN_DROPOUT_PROB = 0.1
let N_HEADS             = 2L
let ATTN_DROPOUT_PROB   = 0.1
let ENCODER_LAYERS      = 2L
let ENCODER_ACTIVATION  = torch.nn.Activations.GELU

// 定义权重初始化函数
let initialize_weights (module_: torch.nn.Module) =
    torch.set_grad_enabled(false) |> ignore// 禁用梯度跟踪
    module_.apply (fun m ->
        match m with
        | :? TorchSharp.Modules.Linear as linear ->
            linear.weight.normal_(0.0, 0.02) |> ignore
            if not (isNull linear.bias) then
                linear.bias.zero_() |> ignore
        | :? TorchSharp.Modules.Embedding as embedding ->
            embedding.weight.normal_(0.0, 0.02) |> ignore
        | :? TorchSharp.Modules.LayerNorm as layer_norm ->
            layer_norm.weight.fill_(1.0) |> ignore
            layer_norm.bias.zero_() |> ignore
        | :? TorchSharp.Modules.Dropout ->
            // Dropout 层没有权重，不需要初始化
            ()
        | _ -> ()
    )  |> ignore
    torch.set_grad_enabled(true)  |> ignore // 重新启用梯度跟踪


let load_model (model: torch.nn.Module) (fileDir: string) =
    let stateDict = Dictionary<string, torch.Tensor>()
    model.state_dict(stateDict) |> ignore

    stateDict
    |> Seq.iter (fun kvp ->
        let filePath = Path.Combine(fileDir, kvp.Key.Replace('/', '_') + ".pt")
        if File.Exists(filePath) then
            let tensor = torch.load(filePath)
            stateDict.[kvp.Key] <- tensor
        else
            printfn "Warning: %s not found" filePath
    )
    
    model.load_state_dict(stateDict) |> ignore



//Note: The module and variable names used here match the tensor name 'paths' as delimted by '/' for TF (see above), 
//for easier matching.
type BertEmbedding(ifInitWeight) as this = 
    inherit torch.nn.Module("embeddings")
    
    let word_embeddings         = torch.nn.Embedding(VOCAB_SIZE,HIDDEN,padding_idx=0L)
    let position_embeddings     = torch.nn.Embedding(MAX_POS_EMB,HIDDEN)
    let token_type_embeddings   = torch.nn.Embedding(TYPE_SIZE,HIDDEN)
    let LayerNorm               = torch.nn.LayerNorm([|HIDDEN|],EPS_LAYER_NORM)
    let dropout                 = torch.nn.Dropout(HIDDEN_DROPOUT_PROB)

    do 
        this.RegisterComponents()
        if ifInitWeight then
            initialize_weights this

    member this.forward(input_ids:torch.Tensor, token_type_ids:torch.Tensor, position_ids:torch.Tensor) =   
    
        let embeddings =      
            (input_ids       --> word_embeddings)        +
            (token_type_ids  --> token_type_embeddings)  +
            (position_ids    --> position_embeddings)

        embeddings --> LayerNorm --> dropout             // the --> operator works for simple 'forward' invocations


type BertPooler() as this = 
    inherit torch.nn.Module<torch.Tensor,torch.Tensor>("pooler")

    let dense = torch.nn.Linear(HIDDEN,HIDDEN)
    let activation = torch.nn.Tanh()

    let ``:`` = torch.TensorIndex.Colon
    let first = torch.TensorIndex.Single(0L)

    do
        this.RegisterComponents()

    override _.forward (hidden_states) =
        let first_token_tensor = hidden_states.index(``:``, first) //take first token of the sequence as the pooled value
        first_token_tensor --> dense --> activation

let dense = torch.nn.Linear(HIDDEN,HIDDEN)
dense.GetType().FullName

torch.nn.Embedding(VOCAB_SIZE,HIDDEN,padding_idx=0L).GetType().FullName


type BertModel(ifInitWeight) as this =
    inherit torch.nn.Module("bert")

    let embeddings = new BertEmbedding(ifInitWeight)
    let pooler = new BertPooler()

    let encoderLayer = torch.nn.TransformerEncoderLayer(HIDDEN, N_HEADS, MAX_POS_EMB, ATTN_DROPOUT_PROB, activation=ENCODER_ACTIVATION)
    let encoder = torch.nn.TransformerEncoder(encoderLayer, ENCODER_LAYERS)

    do
        this.RegisterComponents()
    
    member this.forward(input_ids:torch.Tensor, token_type_ids:torch.Tensor, position_ids:torch.Tensor,?mask:torch.Tensor) =
        let src = embeddings.forward(input_ids, token_type_ids, position_ids)
        let srcBatchDim2nd = src.permute(1L,0L,2L) //PyTorch transformer requires input as such. See the Transformer docs
        let encoded = match mask with None -> encoder.forward(srcBatchDim2nd, null, null) | Some mask -> encoder.forward(srcBatchDim2nd,mask, null)
        let encodedBatchFst = encoded.permute(1L,0L,2L)
        encodedBatchFst --> pooler


let ifInitWeight = (DirectoryInfo weightDataDir).GetFiles().Length = 0

let testBert = (new BertModel(ifInitWeight)).``to``(device)

if not ifInitWeight then
    load_model testBert weightDataDir
//bert.named_modules() 
testBert.named_parameters() |> Seq.map (fun struct(n,x) -> n,x.shape) |> Seq.iter (printfn "%A")


let (struct (pm0, pm1)) = testBert.named_parameters() |> Seq.item 0
//pm0.GetType().Name //string
//pm1.GetType().Name

//pm1.name

open System
module Tensor = 
    //Note: ensure 't matches tensor datatype otherwise ToArray might crash the app (i.e. exception cannot be caught)
    let private _getData<'t when 't:>ValueType and 't: unmanaged and 't:struct and 't : (new:unit->'t) > (t:torch.Tensor) =
        let s = t.data<'t>()
        s.ToArray()

    let getData<'t when 't:>ValueType and 't: unmanaged and 't:struct and 't : (new:unit->'t)>  (t:torch.Tensor) =
        if t.device_type <> DeviceType.CPU then 
            //use t1 = t.clone()
            use t2 = t.cpu()
            _getData<'t> t2
        else 
            _getData<'t> t
  
    let setData<'t when 't:>ValueType and 't: unmanaged and 't:struct and 't : (new:unit->'t)> (t:torch.Tensor) (data:'t[]) =
        if t.device_type = DeviceType.CPU |> not then failwith "tensor has to be on cpu for setData"        
        let s = t.data<'t>()
        s.CopyFrom(data,0,0L)

type PostProc = V | H | T | N

let postProc (ts:torch.Tensor list) = function
    | V -> torch.vstack(ResizeArray ts)
    | H -> torch.hstack(ResizeArray ts)
    | T -> ts.Head.T                  //Linear layer weights need to be transformed. See https://github.com/pytorch/pytorch/issues/2159
    | N -> ts.Head

//let nameMap =
//    [
//        "encoder.layers.#.self_attn.in_proj_weight",["encoder/layer_#/attention/self/query/kernel"; 
//                                                     "encoder/layer_#/attention/self/key/kernel";    
//                                                     "encoder/layer_#/attention/self/value/kernel"],        V
//
//        "encoder.layers.#.self_attn.in_proj_bias",  ["encoder/layer_#/attention/self/query/bias";
//                                                     "encoder/layer_#/attention/self/key/bias"; 
//                                                     "encoder/layer_#/attention/self/value/bias"],          H
//
//        "encoder.layers.#.self_attn.out_proj.weight", ["encoder/layer_#/attention/output/dense/kernel"],    N
//        "encoder.layers.#.self_attn.out_proj.bias",   ["encoder/layer_#/attention/output/dense/bias"],      N
//
//
//        "encoder.layers.#.linear1.weight",          ["encoder/layer_#/intermediate/dense/kernel"],          T
//        "encoder.layers.#.linear1.bias",            ["encoder/layer_#/intermediate/dense/bias"],            N
//
//        "encoder.layers.#.linear2.weight",          ["encoder/layer_#/output/dense/kernel"],                T
//        "encoder.layers.#.linear2.bias",            ["encoder/layer_#/output/dense/bias"],                  N
//
//        "encoder.layers.#.norm1.weight",            ["encoder/layer_#/attention/output/LayerNorm/gamma"],   N
//        "encoder.layers.#.norm1.bias",              ["encoder/layer_#/attention/output/LayerNorm/beta"],    N
//
//        "encoder.layers.#.norm2.weight",            ["encoder/layer_#/output/LayerNorm/gamma"],             N
//        "encoder.layers.#.norm2.bias",              ["encoder/layer_#/output/LayerNorm/beta"],              N
//
//        "embeddings.word_embeddings.weight"         , ["embeddings/word_embeddings"]           , N
//        "embeddings.position_embeddings.weight"     , ["embeddings/position_embeddings"]       , N
//        "embeddings.token_type_embeddings.weight"   , ["embeddings/token_type_embeddings"]     , N
//        "embeddings.LayerNorm.weight"               , ["embeddings/LayerNorm/gamma"]           , N
//        "embeddings.LayerNorm.bias"                 , ["embeddings/LayerNorm/beta"]            , N
//        "pooler.dense.weight"                       , ["pooler/dense/kernel"]                  , T
//        "pooler.dense.bias"                         , ["pooler/dense/bias"]                    , N
//    ]

let PREFIX = "bert"
let addPrefix (s:string) = $"{PREFIX}/{s}"
let sub n (s:string) = s.Replace("#",string n)

//create a PyTorch tensor from TF checkpoint tensor data
let toFloat32Tensor (shpdTnsr:CheckpointReader.ShapedTensor) = 
    match shpdTnsr.Tensor with
    | CheckpointReader.TensorData.TdFloat ds -> torch.tensor(ds, dimensions=shpdTnsr.Shape)
    | _                                      -> failwith "TdFloat expected"

// //set the value of a single parameter
// let performMap (tfMap:Map<string,_>) (ptMap:Map<string,Modules.Parameter>) (torchName,tfNames,postProcType) = 
//     let torchParm = ptMap.[torchName]
//     let fromTfWts = tfNames |> List.map (fun n -> tfMap.[n] |> toFloat32Tensor) 
//     let parmTensor = postProc fromTfWts postProcType
//     if torchParm.shape <> parmTensor.shape then failwithf $"Mismatched weights for parameter {torchName}; parm shape: %A{torchParm.shape} vs tensor shape: %A{parmTensor.shape}"
//     Tensor.setData<float32> torchParm (Tensor.getData<float32>(parmTensor))

// //set the parameter weights of a PyTorch model given checkpoint and nameMap
// let loadWeights (model:torch.nn.Module) checkpoint encoderLayers nameMap =
//     let tfMap = checkpoint |> Map.ofSeq
//     let ptMap = model.named_parameters() |> Seq.map (fun struct(n,m) -> n,m) |> Map.ofSeq

//     //process encoder layers
//     for l in 0 .. encoderLayers - 1 do
//         nameMap
//         |> List.filter (fun (p:string,_,_) -> p.Contains("#")) 
//         |> List.map (fun (p,tns,postProc) -> sub l p, tns |> List.map (addPrefix >> (sub l)), postProc)
//         |> List.iter (performMap tfMap ptMap)

//     nameMap
//     |> List.filter (fun (p,_,_) -> p.Contains("#") |> not)
//     |> List.map (fun (name,tns,postProcType) -> name, tns |> List.map addPrefix, postProcType)
//     |> List.iter (performMap tfMap ptMap)

//loadWeights testBert tensors (int ENCODER_LAYERS) nameMap


let l0w = testBert.get_parameter("encoder.layers.0.self_attn.in_proj_weight") |> Tensor.getData<float32>


//tensors |> Seq.find (fun (n,_) -> n = "bert/encoder/layer_0/attention/self/query/kernel")


//Training Data
//The training dataset is the [Yelp review dataset](https://s3.amazonaws.com/fast-ai-nlp/yelp_review_polarity_csv.tgz). Assume this data is saved to a local folder as given below.

open System.IO
let foldr = @"C:\anibal\ttc\yelp_review_all_csv"
let testCsv = Path.Combine(foldr,"test.csv")
let trainCsv = Path.Combine(foldr,"train.csv")
if File.Exists testCsv |> not then failwith $"File not found; path = {testCsv}"
printfn "%A" trainCsv


open FSharp.Data
type YelpCsv = FSharp.Data.CsvProvider<Sample="a,b", HasHeaders=false, Schema="Label,Text">
type [<CLIMutable>] YelpReview = {Label:int; Text:string}
//need to make labels 0-based so subtract 1
let testSet = YelpCsv.Load(testCsv).Rows |> Seq.map (fun r-> {Label=int r.Label - 1; Text=r.Text}) |> Seq.toArray 
let trainSet = YelpCsv.Load(trainCsv).Rows |> Seq.map (fun r->{Label=int r.Label - 1; Text=r.Text}) |> Seq.toArray

open FSharp.Collections
let classes = trainSet |> Seq.map (fun x->x.Label) |> set
//classes.Display()
let TGT_LEN = classes.Count |> int64



let BATCH_SIZE = 128

let batchDict = ConcurrentDictionary<string * int, YelpReview[]>()

let trainSetIDs =
    trainSet 
    |> Array.chunkBySize BATCH_SIZE 
    |> Array.mapi (fun i c -> 
        let _ = batchDict.TryAdd (("train", i), c)
        "train", i
    )
    

//trainBatches.GetType().FullName
//trainBatches |> Seq.length

let testSetIDs =
    testSet  
    |> Array.chunkBySize BATCH_SIZE 
    |> Array.mapi (fun i c -> 
        let _ = batchDict.TryAdd (("test", i), c)
        "test", i
    )



open BERTTokenizer
let vocabFile = @"C:\anibal\ttc\vocab.txt"
let vocab = Vocabulary.loadFromFile vocabFile

let position_ids = torch.arange(MAX_POS_EMB).expand(int64 BATCH_SIZE,-1L).``to``(device)


//position_ids.data<int64>() //seq [0L; 1L; 2L; 3L; ...]

let b2f (b:YelpReview): Features =
    Featurizer.toFeatures vocab true (int MAX_POS_EMB) b.Text ""

b2f {Label=1;Text = "orz OGC"}

//convert a batch to input and output (X, Y) tensors


let featureDict = ConcurrentDictionary<string * int, Features[]>()
let labelDict = ConcurrentDictionary<string * int, int[]>()

let featurePickle = Path.Join($"{weightDataDir}.pickle","feature")
let labelPickle = Path.Join($"{weightDataDir}.pickle","label")

let toFeature (batchId:string * int) = 
    featureDict.GetOrAdd(
        batchId
        , fun k -> 
            let fi = Path.Join(featurePickle, string (snd k) + ".pickle") |> FileInfo
            if fi.Exists then
                binarySerializer.UnPickle (File.ReadAllBytes fi.FullName)
            else
                let f = batchDict[k] |> Array.map b2f
                File.WriteAllBytes(fi.FullName, binarySerializer.Pickle f)                
                f
    )
    

let toLabel (batchId:string * int) = 
    labelDict.GetOrAdd(
        batchId
        , fun k -> 
            let fi = Path.Join(labelPickle, string (snd k) + ".pickle") |> FileInfo
            if fi.Exists then
                binarySerializer.UnPickle (File.ReadAllBytes fi.FullName)
            else
                let l = batchDict[k] |> Array.map (fun x->x.Label)
                File.WriteAllBytes(fi.FullName, binarySerializer.Pickle l)                
                l
    )
    




let toXY (batchId:string * int) =
    let xs = toFeature batchId
    let d_tkns      = xs |> Seq.collect (fun f -> f.InputIds )  |> Seq.toArray
    let d_tkn_typs  = xs |> Seq.collect (fun f -> f.SegmentIds) |> Seq.toArray
    let tokenIds = torch.tensor(d_tkns,     dtype=torch.int).view(-1L,MAX_POS_EMB).``to``(device)        
    let sepIds   = torch.tensor(d_tkn_typs, dtype=torch.int).view(-1L,MAX_POS_EMB).``to``(device)
    let Y = torch.tensor(toLabel batchId , dtype=torch.int64).view(-1L).``to``(device)
    (tokenIds,sepIds),Y


testBert.eval()
let (_tkns,_seps),_ = toXY ("train", 0)
//_tkns.shape
//_tkns |> Tensor.getData<int64>
let _testOut = testBert.forward(_tkns,_seps,position_ids) //test is on cpu --->  position_ids.cpu()



type BertClassification(ifInitWeight) as this = 
    inherit torch.nn.Module("BertClassification")

    let bert = new BertModel(ifInitWeight)
    let proj = torch.nn.Linear(HIDDEN,TGT_LEN)

    do
        this.RegisterComponents()
        //this.LoadBertPretrained()
        //if ifInitWeight then
        //    initialize_weights bert |> ignore

    member _.LoadBertPretrained(preTrainedDataFilePath) =
        load_model bert preTrainedDataFilePath 
    //     loadWeights bert tensors (int ENCODER_LAYERS) nameMap
    
    member _.forward(tknIds,sepIds,postionIds) =
        use encoded = bert.forward(tknIds,sepIds,postionIds)
        encoded --> proj 


let _model = new BertClassification(ifInitWeight)
_model.``to``(device) |> ignore

if not ifInitWeight then
    _model.LoadBertPretrained weightDataDir
//let _model2 = new BertClassification()

//_model2.eval()



let _loss = torch.nn.CrossEntropyLoss()
let mutable EPOCHS = 1
let mutable verbose = true
let gradCap = 0.1f
let gradMin,gradMax = (-gradCap).ToScalar(),  gradCap.ToScalar()
let opt = torch.optim.Adam(_model.parameters (), 0.001, amsgrad=true)  
   

let class_accuracy (y:torch.Tensor) (y':torch.Tensor) =
    use i = y'.argmax(1L)
    let i_t = Tensor.getData<int64>(i)
    let m_t = Tensor.getData<int64>(y)
    Seq.zip i_t m_t 
    |> Seq.map (fun (a,b) -> if a = b then 1.0 else 0.0) 
    |> Seq.average

//adjustment for end of data when full batch may not be available
let adjPositions currBatchSize = if int currBatchSize = BATCH_SIZE then position_ids else torch.arange(MAX_POS_EMB).expand(currBatchSize,-1L).``to``(device)

let dispose ls = ls |> List.iter (fun (x:IDisposable) -> x.Dispose())

//run a batch through the model; return true output, predicted output and loss tensors
let processBatch ((tkns:torch.Tensor,typs:torch.Tensor), y:torch.Tensor) =
    use tkns_d = tkns.``to``(device)
    use typs_d = typs.``to``(device)
    let y_d    = y.``to``(device)            
    let postions  = adjPositions tkns.shape.[0]
    if device <> torch.CPU then //these were copied so ok to dispose old tensors
        dispose [tkns; typs; y]
    let y' = _model.forward(tkns_d,typs_d,postions)
    let loss = _loss.forward(y', y_d)   
    y_d,y',loss



//evaluate on test set; return cross-entropy loss and classification accuracy
let evaluate e =
    _model.eval()
    let lss =
        testSetIDs 
        |> Seq.map toXY
        |> Seq.map (fun batch ->
            let y,y',loss = processBatch batch
            let ls = loss.ToDouble()
            let acc = class_accuracy y y'            
            dispose [y;y';loss]
            GC.Collect()
            ls,acc)
        |> Seq.toArray
    let ls  = lss |> Seq.averageBy fst
    let acc = lss |> Seq.averageBy snd
    ls,acc




let mutable e = 0
let train () =
    
    while e < EPOCHS do
        e <- e + 1
        _model.train()
        
        printfn $"Starting: toxyed"
        let toxyed = 
            trainSetIDs 
            |> Seq.mapi (fun i b -> 
                if i % 20 = 0 then
                    printfn $"batch {b}"
                toXY b
            ) 
        //let batch = toxyed |> Seq.item 0
        
        printfn "trainBatches length: %d" (trainSetIDs.Length)
        let losses = 
            toxyed
            |> Seq.mapi (fun i batch ->                 
                opt.zero_grad ()   
                let y, y', loss = processBatch batch
                let ls = loss.ToDouble()  
                loss.backward()
                _model.parameters() |> Seq.iter (fun t -> t.grad().clip(gradMin,gradMax) |> ignore)                            
                use  t_opt = opt.step ()
                if verbose && i % 100 = 0 then
                    let acc = class_accuracy y y'
                    printfn $"Epoch: {e}, minibatch: {i}, ce: {ls}, accuracy: {acc}"                            
                dispose [y;y';loss]
                GC.Collect()
                ls)
            |> Seq.toArray

        let evalCE,evalAcc = evaluate e
        printfn $"Epoch {e} train: {Seq.average losses}; eval acc: {evalAcc}"

    printfn "Done train"

let runner () = async { do train () } 


// open System
// async {
//     printfn "%s" (DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
//     train ()
//     printfn "%s" (DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
// }
// |> Async.RunSynchronously

//2024-06-01 16:34:19
//e <- 0
open System
open System.Collections.Generic

let ts = DateTime.Now
printfn "%s" (ts.ToString("yyyy-MM-dd HH:mm:ss"))
train ()
let te = DateTime.Now
printfn "%s" (te.ToString("yyyy-MM-dd HH:mm:ss"))





let save_model (model: torch.nn.Module) (fileDir: string) =
    //use stream = System.IO.File.Create(filePath)
    let stateDict = Dictionary<string, torch.Tensor> ()

    model.state_dict(stateDict) |> ignore
    stateDict
    |> Seq.iter (fun kvp ->
        torch.save(kvp.Value,Path.Join(fileDir, kvp.Key.Replace('/', '_') + ".pt"))        
    )



save_model _model @"C:\anibal\ttc\MyTrainedModel\yelp_review_all_csv"


//Console.ReadLine() |> ignore
//#!fsharp

//123

//#!fsharp

//let b1xy = testBatches |> Seq.item 0 |> toXY

//#!fsharp

//(snd (fst b1xy)).GetType().Name

//#!fsharp

//(testBatches |> Seq.item 0).GetType().Name

//#!fsharp

//(testBatches |> Seq.item 0).[0].Label

//#!fsharp

//(testBatches |> Seq.item 0).[0].Text

//#!fsharp

//opt.zero_grad ()   
//let y,y',loss = processBatch b1xy
//let ls = loss.ToDouble()  

//#!fsharp

//loss.backward()
//_model.parameters() |> Seq.iter (fun t -> t.grad().clip(gradMin,gradMax) |> ignore)                            
//use  t_opt = opt.step ()

//#!fsharp

//loss

//#!fsharp

////if verbose && i % 100 = 0 then
//let acc = class_accuracy y y'
//printfn $"Epoch: {e}, minibatch: {0}, ce: {ls}, accuracy: {acc}"                            
//dispose [y;y';loss]
//GC.Collect()
//ls

//#!fsharp

//let y,y',loss = processBatch batch
//let ls = loss.ToDouble()
//let acc = class_accuracy y y' 
